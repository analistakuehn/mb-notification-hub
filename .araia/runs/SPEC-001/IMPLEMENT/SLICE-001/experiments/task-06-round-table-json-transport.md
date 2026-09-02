# Mesa redonda: fronteira de confiança do transporte JSON do AWS CLI

**Tarefa**: 6 (experimento de identidade e proteção do objeto sob custódia)  
**Convocada em**: 2026-09-01T22:05:00Z  
**Mediador**: `dotnet-architect`  
**Participantes obrigatórios**: `dotnet-engineer`, `dotnet-specialist`  
**Dono da decisão**: usuário (delegação explícita nesta sessão: "tome as melhores decisões por mim; em caso de dúvidas convoque uma mesa redonda; considere tudo previamente aprovado")

## Brief

### Pergunta de decisão

Qual conjunto de controles locais do sistema de arquivos torna o transporte por arquivo dos argumentos JSON do AWS CLI aceitável como fail-closed dentro de uma fronteira de ameaça explícita, e qual é essa fronteira, de modo que a 25ª revisão independente avalie conformidade com uma fronteira fixada em vez de introduzir classes de ameaça novas a cada rodada?

### Contexto imutável

- O runbook `task-06-aws-matrix.ps1` (5.530 linhas, SHA-256 `beb950d8e61653cabeed617a55f4333a11601baa01710e2263db8550cb84312e`) converte nove parâmetros JSON em arquivos UTF-8 sem BOM dentro de `%LOCALAPPDATA%\Araia\Task6\<RunId>\aws-cli-json` e entrega ao AWS CLI 2.36.3 um URI `file://` absoluto. O AWS CLI abre o arquivo por caminho absoluto; não há como entregar o documento por handle herdado ou por stdin.
- O lease atual (`New-AwsCliArgumentLease`, linhas 346 a 494) abre `<RunId>` e `aws-cli-json` com `FILE_SHARE_READ | FILE_SHARE_WRITE`, abre o arquivo com `FILE_SHARE_READ`, verifica o SHA-256 pelo próprio handle e mantém os handles até o término do processo.
- A 24ª revisão retornou arquitetura `READY` e especialidade `BLOCKED` (linha 61 de `.araia/refusal-log.jsonl`): somente os componentes finais recebem handles; os handles de diretório permitem compartilhamento de escrita; um ponto de nova análise em um componente ancestral, ou aplicado durante o lease, pode redirecionar o URI consumido pelo CLI. O ensaio cobre apenas junction preexistente no componente final e substituição do arquivo.
- A execução AWS está autorizada (teto USD 0,25, duas CMKs em `PendingDeletion` por sete dias, resíduo temporário de Compliance). Nenhum recurso cobrável existe; a limpeza da tentativa parcial foi verificada (`cleanup-verified-partial-provision`).
- Vinte e quatro rodadas de revisão ocorreram em 2026-09-01. Cada rodada de remediação e revisão custa entre 20 e 40 minutos.

### Evidência Win32 reproduzível (sonda `win32-chain-spike.ps1`, PowerShell 7.6.5, Windows 11 26200, NTFS)

| Hipótese | Procedimento | Resultado |
|---|---|---|
| H1 | Abrir âncora por caminho absoluto e cada componente seguinte com `NtCreateFile` relativo ao handle anterior (`RootDirectory`), `FILE_OPEN_REPARSE_POINT`, `FILE_SHARE_READ` | Cadeia aberta; leitura pelo handle relativo devolveu os bytes gravados |
| H1b | Abrir o mesmo arquivo pelo caminho absoluto e comparar `VolumeSerialNumber` e `FileIndex` | Identidades iguais |
| H2 | Renomear ou excluir componentes mantidos durante a cadeia | `IOException` em todos os casos |
| H3 | `CreateFile` com `GENERIC_WRITE` sobre diretório mantido com `FILE_SHARE_READ` | Erro 32 (`ERROR_SHARING_VIOLATION`) |
| H4 | Liberar o handle da âncora e renomear a âncora enquanto descendentes permanecem abertos | `IOException`: NTFS recusa renomear diretório com handles abertos na subárvore |
| H5 | Sobrescrever o arquivo mantido | `MethodInvocationException` (compartilhamento negado) |
| H6 | Abrir junction por `NtCreateFile` relativo com `FILE_OPEN_REPARSE_POINT` | Atributos `0x410` (`REPARSE_POINT` e `DIRECTORY`), detectável antes de qualquer uso |
| H7 | `Environment.GetFolderPath(LocalApplicationData)` | Igual a `%LOCALAPPDATA%`, resolvido pela API de pastas conhecidas |

Fatos normativos do NTFS usados pelo raciocínio: `FSCTL_SET_REPARSE_POINT` exige handle com acesso de escrita e diretório vazio; renomear diretório exige `DELETE` sobre ele e é recusado quando existem handles abertos na subárvore.

### Alternativas

- **A1. Cadeia mantida a partir de âncora validada**: a âncora é a pasta conhecida `LocalApplicationData`, validada por handle (existe, é diretório, não é ponto de nova análise, unidade fixa local) e não mantida. Cada componente abaixo (`Araia`, `Task6`, `<RunId>`, `aws-cli-json`, arquivo) é aberto com `NtCreateFile` relativo ao handle anterior, `FILE_OPEN_REPARSE_POINT`, sem compartilhamento de escrita ou exclusão, verificado por handle e mantido até o término do processo. A identidade do arquivo pelo caminho absoluto é comparada com a do handle relativo no momento do lease. Resíduo aceito: atacante com a mesma identidade de usuário (pode substituir o runbook, o cache de credenciais ou o mapa de dispositivos DOS) e ancestrais acima da âncora.
- **A2. A1 mais handle exclusivo na âncora**: idêntica a A1, mantendo também `LocalApplicationData` sem compartilhamento de escrita. Risco colateral: outros processos que abram a pasta conhecida com acesso de escrita durante a execução falham; H4 mostra que a proteção contra renomeação já decorre dos handles abaixo.
- **A3. Abandonar o transporte por arquivo**: o AWS CLI não aceita stdin nem handle herdado para esses parâmetros; a sintaxe abreviada não cobre documentos de política IAM e KMS. Requer reescrever os nove usos e reabrir as revisões 23 e 24.
- **A4. Aceitar a versão atual sem alteração**: documentar o resíduo e executar. Contraria o parecer `BLOCKED` da especialidade e deixa os diretórios com compartilhamento de escrita.

### Critérios de decisão

1. Fechamento fail-closed contra troca do documento entre a verificação do hash e a leitura pelo AWS CLI, para qualquer atacante que não possua a identidade do usuário.
2. Ausência de efeito colateral sobre processos alheios ao experimento.
3. Oráculos falsificáveis no ensaio local, sem chamadas AWS.
4. Tamanho da mudança compatível com uma única rodada de revisão.
5. Fronteira de ameaça explícita, escrita, que a 25ª revisão adota como critério de aceitação.

### Restrições

- Sem novas dependências; PowerShell 7.6.5 com `Add-Type` C# inline, como o runbook já faz.
- Nenhuma chamada AWS antes de `READY` das duas revisões.
- Texto incorporado em pt-BR com diacríticos; identificadores em inglês.
- O runbook continua com uma única tentativa do AWS CLI por mutação e com o estado autenticado na versão 6.

### Decisões já aceitas (não reabrir)

- Transporte por arquivo em diretório restrito com ACL exclusiva (revisões 23 e 24).
- Convergência de falhas locais de preparação para `not-applied` com código local 252 (revisão 24).
- Estado autenticado por HMAC com chave protegida por DPAPI.

### Forma de retorno solicitada

`RECOMMEND`, `NO-CONSENSUS` ou `INSUFFICIENT-EVIDENCE`, com: alternativa líder; fronteira de ameaça aceita (o que está dentro e o que está fora); conjunto mínimo de controles obrigatórios; oráculos obrigatórios do ensaio; resíduos a documentar no experimento; dissenso preservado.

## Abertura do mediador (`dotnet-architect`)

Fontes: brief, runbook, ensaio, sonda (reexecutada, resultados idênticos a H1 a H7), refusal-log linha 61, histórico. Nada alterado; nenhuma chamada AWS.

### Fatos condicionantes

- F1. O runbook deriva `$StateRoot` de `$env:LOCALAPPDATA` (linha 27), não da API de pastas conhecidas que A1 pressupõe. Coincidem hoje (H7); as fontes diferem.
- F2. `Araia` e `Task6` não recebem ACL restrita nem handle. `<RunId>` e `aws-cli-json` têm ACL lida por caminho e são mantidos com `FILE_SHARE_WRITE`; `OpenLocked` detecta nova análise só no objeto final.
- F3. O ensaio enraíza `$StateRoot` em `%TEMP%`; a cadeia produtiva `Araia\Task6\<RunId>` nunca é exercitada.
- F4. O `BLOCKED` da 24ª revisão nomeia o mecanismo (nova análise em ancestral; janela com `FILE_SHARE_WRITE`), mas não o principal capaz de exercê-lo sem a identidade do usuário sob as ACLs vigentes.

### Pergunta de decisão normalizada

Qual fronteira de ameaça escrita e qual conjunto mínimo de controles Win32 sobre cada componente entre uma âncora definida e o arquivo JSON tornam o transporte por arquivo fail-closed contra a troca do documento entre o hash e a leitura pelo AWS CLI, de modo que a 25ª revisão julgue conformidade com essa fronteira em vez de ampliá-la?

### Alternativas comparáveis

- A1. Cadeia mantida a partir de âncora validada (`LocalApplicationData` validada e liberada; componentes abaixo abertos por `NtCreateFile` relativo, `FILE_OPEN_REPARSE_POINT`, sem compartilhamento de escrita ou exclusão, mantidos; identidade absoluta comparada à do handle relativo). Resíduo: mesma identidade de usuário e ancestrais acima da âncora.
- A2. A1 mais âncora mantida sem compartilhamento de escrita. H4 mostra que a renomeação da âncora já é recusada pelos handles abaixo; ganho residual e efeito colateral não quantificados.
- A3. Desqualificada: contraria a decisão aceita do transporte por arquivo (revisões 23 e 24).
- A4. Desqualificada enquanto vigorar "nenhuma chamada AWS antes de `READY` das duas revisões" com o `BLOCKED` sobre o hash `beb950d8`. Volta à mesa só se S1 esvaziar o bloqueio.

### Critérios e precedência

1. Fechamento fail-closed contra troca do documento entre hash e leitura pelo CLI, para atacante sem a identidade do usuário.
2. Fronteira de ameaça explícita e escrita, adotada pela 25ª revisão como critério de aceitação.
3. Oráculos falsificáveis no ensaio local, sem AWS.
4. Ausência de efeito colateral sobre processos alheios.
5. Tamanho compatível com uma única rodada de revisão.

Os critérios 1 a 3 eliminam; 4 e 5 ordenam o que sobrar.

### Restrições desqualificantes

Sem novas dependências; PowerShell 7.6.5 com `Add-Type` inline. Nenhuma chamada AWS antes de `READY` das duas revisões. Não reabrir: transporte por arquivo com ACL exclusiva; convergência para `not-applied` com código 252; estado autenticado HMAC com DPAPI na versão 6. Uma única tentativa do CLI por mutação. Texto incorporado em pt-BR com diacríticos; identificadores em inglês.

### Perguntas aos participantes

`dotnet-engineer`: E1 (âncora: `$env:LOCALAPPDATA`, `GetFolderPath(LocalApplicationData)` ou ambos cruzados; persistência sem alterar o esquema 6); E2 (lista de componentes literal ou derivada; como o ensaio exercita a cadeia produtiva); E3 (ACL por handle ou por caminho; janela restante); E4 (oráculos obrigatórios e cobertura atual); E5 (estimativa de linhas).

`dotnet-specialist`: S1 (principal capaz de explorar o mecanismo sem a identidade do usuário sob as ACLs vigentes; fechamento de ameaça realizável ou defesa em profundidade); S2 (âncora mantida ou validada; ataque que A2 fecha e A1 deixa aberto; processo real prejudicado); S3 (resíduos: mesma identidade, mapa de dispositivos DOS, redirecionamento de pasta conhecida, ancestrais acima da âncora; forma de registro); S4 (comparação de identidade basta, ou a cadeia mantida é a única prova válida).

25ª revisão (ambos): R1 (checklist fechado); R2 (classes fora da fronteira que viram resíduo registrado); R3 (o que reabre a revisão).

### Lacunas de evidência

- L1 (F4): sem o principal nomeado em S1, a mesa não distingue fechamento de ameaça de defesa em profundidade, e A4 fica indecidível.
- L2: o efeito colateral de A2 é inferência a partir de H3, não observação.
- L3: H4 mediu só o pai imediato; ancestrais mais altos dependem do fato normativo do NTFS, citado e não medido.
- L4: nenhum oráculo mede a janela entre verificação por caminho e abertura por handle (E3).

L1 e L4 impedem a síntese; L2 e L3 só condicionam a ordem entre A1 e A2.


## Posição do engenheiro (`dotnet-engineer`)

**Veredito**: `RECOMMEND` com bloqueio de procedimento. Alternativa líder: A1 na forma estreita, com a âncora `LocalApplicationData` validada e liberada e a cadeia mantida apenas de `Araia` para baixo. Rejeita A2, A3 e A4.

### Fato bloqueante: o objeto sob revisão mudou durante a mesa

O contexto imutável do brief descreve `task-06-aws-matrix.ps1` com 5.530 linhas e SHA-256 `beb950d8`. Não é o que está no disco. Medições somente leitura:

```
task-06-aws-matrix.ps1                 257a38bd...f420    5947 linhas   mtime 2026-09-01T22:30:24Z
task-06-aws-matrix.ps1                 17f3bba4...05d9    5988 linhas   mtime 2026-09-01T22:34:30Z
task-06-aws-json-transport-tests.ps1   e7841672...a492     820 linhas   mtime 2026-09-01T22:30:24Z
task-06-aws-json-transport-tests.ps1   28f49893...852e2    821 linhas   mtime 2026-09-01T22:34:40Z
```

A mesa foi convocada às 22:05Z e a abertura foi escrita às 22:13Z. O parecer `BLOCKED` da 24ª revisão está fixado em `beb950d8`, anterior às duas alterações. Portanto os fatos F1 a F4 e as lacunas L1 a L4 foram computados sobre um artefato que não existe mais.

Exigência de procedimento: congelar o runbook e o ensaio, publicar o par de hashes na ficha da mesa e proibir edição até o veredito. Sem isso, a rodada 25 repete o padrão que motivou esta mesa.

### Estado realmente observado, que remede F2 e F3

Sobre `17f3bba4`, o implementado é mais forte que A1 e mais forte que A2: cadeia derivada do caminho desde a raiz do volume, com nove handles de diretório; `FILE_SHARE_WRITE` não existe mais no código; rejeição de nova análise em todo componente; recusa de ponto de montagem por `GetVolumePathName`; revalidação por `Assert-AwsCliArgumentLeaseCurrent` antes de cada criação de processo; continuidade de identidade por `FileId` na janela do `icacls`.

F2 está obsoleto. F1 continua verdadeiro, porque a linha 27 deriva de `$env:LOCALAPPDATA` e não há `GetFolderPath` no runbook. F3 continua verdadeiro, porque o ensaio enraíza em `%TEMP%`. O problema atual não é falta de controle: é excesso de controle mal delimitado, sem fronteira escrita.

### Por que A1 estreita

Manter `C:\`, `C:\Users`, o perfil, `AppData` e `Local` não compra fechamento e cobra colateral. A negação de renomeação de qualquer ancestral decorre da regra de subárvore do NTFS, disparada pelo componente mais profundo. A negação de conversão em nova análise, para ancestral não mantido, decorre da exigência de diretório vazio. A verificação de identidade não exige manter. Sobram apenas os custos: um handle de diretório mantido recusa toda renomeação cujo destino seja ele, e `%LOCALAPPDATA%` tem 68 subdiretórios vizinhos onde a gravação atômica é o padrão.

Contagem medida: a enumeração de subdiretórios de `LocalApplicationData` devolveu 68.

### E1. Âncora

As duas fontes, cruzadas, e nenhuma delas persistida no estado. Resolver `GetFolderPath(LocalApplicationData, DoNotVerify)` e `$env:LOCALAPPDATA`, canonicalizar e comparar; divergência falha fechado com `StateAnchorDivergence` antes de qualquer escrita de estado. Medição: as duas fontes coincidem hoje nesta máquina.

A âncora não pode ser autenticada pelo `state.json`, porque o estado mora dentro dela e qualquer campo seria circular. A persistência correta é o próprio runbook, coberto pelo hash da revisão, mais o registro de caminho, `VolumePath` e `VolumeName` dentro do lease, mais um evento em `restricted-evidence.jsonl`, que não faz parte do payload com HMAC. O esquema 6 não é tocado.

### E2. Lista de componentes e cobertura do ensaio

Derivada, com asserção literal de cauda por comparação ordinal sobre `Araia`, `Task6`, o `RunId` autorizado e `aws-cli-json`, para impedir que uma redefinição silenciosa de `$StateRoot` mude o destino enquanto o código de cadeia continua passando.

Sobre F3, o ensaio não pode enraizar no diretório de estado autenticado da execução autorizada, sob pena de contaminar o HMAC e a chave DPAPI da corrida real. A cobertura correta tem três partes: montar a mesma forma sob raiz descartável com a mesma profundidade de cauda; transformar a resolução da âncora em ponto de injeção; e verificar a composição produtiva como função pura, sem tocar o disco.

### E3. ACL por handle ou por caminho

Por handle é correto em princípio e não deve ser feito nesta rodada. Com a cadeia mantida e a identidade comparada, a leitura por caminho não pode atingir objeto diferente do que está no handle. Ler por handle é simplificação de prova, não fechamento de ameaça, e custa `READ_CONTROL` mais uma subclasse de `NativeObjectSecurity`, dezenas de linhas de P/Invoke nova na rodada cujo objetivo é parar de crescer.

A janela que permanece, sob qualquer das duas formas: o DACL pode ser reescrito depois da verificação. Modo de compartilhamento não protege contra `WRITE_DAC`. Quem reescreve o DACL é o proprietário, Administradores ou SYSTEM, todos fora da fronteira. O oráculo honesto não afirma proteção: ele afrouxa o DACL entre o lease e a chamada e registra que nada detecta, fixando o resíduo em código.

### E4. Oráculos

Existentes: O1 calibração positiva do `FSCTL` em diretório vazio; O2 junction preexistente rejeitada; O3 junction em componente ancestral com saída 252; O4 substituição do arquivo recusada; O5 `FSCTL` sobre ancestral durante o lease com erro 32; O6 o processo lê os bytes verificados; O7 arquivo adulterado entre execuções; O8 convergência para `not-applied`; O9 caminho feliz com o parser real.

Faltantes e obrigatórios: M1 cadeia com a forma produtiva sob raiz descartável; M2 calibração negativa do `FSCTL` em diretório não vazio; M3 renomeação e exclusão de ancestral a três e a cinco níveis; M4 mutação de cada campo do lease com recusa nomeada; M5 divergência de âncora; M6 ACE de escrita para terceiro em `Araia` ou `Task6`; M7 medição do colateral de renomeação.

M2, M3 e M7 decidem entre cadeia estreita e cadeia larga. Vêm primeiro; a mudança do runbook vem depois, condicionada ao resultado.

### E5. Estimativa

Cerca de 123 linhas no runbook e 290 no ensaio, aproximadamente 410. Cabe em uma rodada com duas ressalvas: o conjunto tem de ser fechado antes de começar, e a ACL por handle fica fora desta rodada. Com ACL por handle, a estimativa passa a 500 linhas e o engenheiro já não afirma que cabe.

### Modos de falha da própria recomendação

A regra de subárvore pode ter exceção não medida. A exigência de diretório vazio para `FSCTL_SET_REPARSE_POINT` é citada, não medida. A medição de colateral pode não reproduzir o custo real, e nesse caso a diferença entre estreita e larga vira preferência. A verificação de ACE pode reprovar em máquina com política corporativa. O ponto de injeção da âncora no ensaio é ele próprio um afrouxamento. Congelar o artefato não impede que outro agente o edite, apenas torna a edição detectável.

### Objeções às demais alternativas

A2 não fecha ameaça que A1 estreita deixe aberta e é a única que impõe recusa de renomeação atômica em pasta com 68 vizinhos ativos. A implementação em disco é A2 mais quatro níveis acima, o que agrava sem nomear a decisão.

A3 permanece desqualificada. A sintaxe abreviada cobre `--tags`, mas não cobre documento de política IAM nem seletor do CloudTrail. Passar JSON na linha de comando reintroduz exposição por linha de comando de processo, legível por qualquer processo do mesmo usuário e sem ACL. É retrocesso de segurança.

A4 não tem referente: a versão sobre a qual o `BLOCKED` foi emitido não é a versão em disco. Além disso faltam M2, M3 e M4, e a revalidação de hoje pode ter qualquer comparação removida sem que nenhum teste reprove.

## Posição do especialista (`dotnet-specialist`)

**Veredito**: `RECOMMEND: A1` com emenda de escopo, e refutação do próprio parecer `BLOCKED` da 24ª revisão no ponto em que ele afirmava fechar uma ameaça.

### Retificação prévia

O brief descreve artefato que não está mais em disco. A busca por `FileShareWrite`, `0x00000002` e `FILE_SHARE_WRITE` no runbook atual não encontra nenhuma ocorrência. A cadeia é aberta desde a raiz do volume, cada componente com `FILE_SHARE_READ` e `FILE_OPEN_REPARSE_POINT`, rejeitando nova análise. O mecanismo que o especialista nomeou na 24ª revisão já foi remediado. A4 perdeu referente.

O especialista registra um erro próprio desta sessão: leu `OpenDirectory` como `FILE_READ_ATTRIBUTES` e concluiu que o lease de diretório era inerte. `NtQueryObject` sobre os handles reais devolveu `0x00100081`, isto é `SYNCHRONIZE`, `FILE_LIST_DIRECTORY` e `FILE_READ_ATTRIBUTES`. A armadilha desta mesa é exatamente essa: a máscara de acesso, não o modo de compartilhamento, decide se o handle protege alguma coisa, e isso não se julga lendo o texto do código.

### Evidência medida em NTFS local, Windows 11 26200, PowerShell 7.6.5, sem AWS e sem rede

| Id | Medição | Resultado |
|---|---|---|
| M1a | Diretório mantido só com `FILE_READ_ATTRIBUTES` e `FILE_SHARE_READ` | Outro `GENERIC_WRITE`: 0. `DELETE`: 0. Renomear filho: sucesso. Renomear o próprio: sucesso |
| M1c | O mesmo caso somando `FILE_LIST_DIRECTORY` | `GENERIC_WRITE`: 32. `DELETE`: 32. Renomear filho: 32. Renomear o próprio: 32. `FILE_WRITE_ATTRIBUTES`: 0 |
| X | Repetição de M1c a partir de um segundo processo | `GENERIC_WRITE`: 32. `FILE_WRITE_ATTRIBUTES`: 0. Renomear filho: 32 |
| M2a | `FSCTL_SET_REPARSE_POINT` em diretório vazio mantido com `FILE_SHARE_READ` | Sucesso por `GENERIC_WRITE` e por `FILE_WRITE_ATTRIBUTES`; o handle mantido passa a ver `0x410` |
| C5 | Idem, com gravação posterior pelo caminho | A gravação atravessa a junction e cai no alvo do atacante |
| M2b | `FSCTL_SET_REPARSE_POINT` em diretório não vazio | 145, isto é `ERROR_DIR_NOT_EMPTY`, por ambos os acessos |
| M3 e Y | Handle de arquivo `GENERIC_READ` com `FILE_SHARE_READ`, renomeando ancestrais | 32 em todos os níveis, medido de um a oito níveis acima; exclusão recursiva do ancestral: 32; `GENERIC_WRITE` e `DELETE` no arquivo: 32; renomear o arquivo: 32; leitura como o CLI faz: 0 |
| C2 | O mesmo handle contra aberturas dos ancestrais | `GENERIC_WRITE` em ancestral: 0; criar e renomear irmãos: sucesso |
| M4 | Custo de manter diretório com acesso de dados | Renomear neto: ok. Criar e excluir na subárvore: ok. `icacls`: ok. Gravar temporário e renomear dentro do mantido: 32 |
| P2 | Identidade por caminho sem cadeia mantida, seguida de troca do ancestral | O mesmo caminho absoluto passa a resolver para outro `FileId` e devolve outro conteúdo |
| P3 | A mesma troca com o arquivo mantido | 32 |
| B1 e P1 | Lease de produção reproduzido por AST, com `$StateRoot` em diretório temporário | Dez diretórios mantidos, todos com `0x00100081`; arquivo com `0x00120089`. `GENERIC_WRITE` em `C:\` e `C:\Users`: 5, por ACL. Nos demais: 32. `FSCTL` do oráculo: falha de abertura com 32. Renomear ancestral durante o lease: 32. Revalidação passou. Bytes lidos: os verificados |

Sobre o efeito colateral da lacuna L2: um observador de sistema de arquivos não recursivo sobre `%LOCALAPPDATA%` e `C:\` por 60 segundos não registrou nenhum evento. Sessenta segundos não limitam o risco de uma matriz longa. O especialista trata isso como não medido e usa apenas o mecanismo, que está medido.

### S1. Principal capaz de exercer o mecanismo sem a identidade do usuário

Nenhum. A mitigação é defesa em profundidade mais fechamento de uma classe acidental, não fechamento de ameaça contra principal nomeado.

- Outra conta não administrativa da máquina: a ACL do perfil não lhe dá travessia. Incapaz.
- Processo do mesmo usuário em integridade baixa ou em AppContainer: a política de integridade obrigatória proíbe escrita em objeto de integridade média. Incapaz.
- Processo do mesmo usuário em integridade média: capaz, e possui a identidade do usuário por definição. É o resíduo já aceito, e o mesmo principal substitui o runbook, o cache de credenciais ou o bloco de ambiente sem precisar de nova análise.
- SYSTEM, Administradores e portadores de `SeBackupPrivilege`, `SeRestorePrivilege`, `SeTakeOwnershipPrivilege` ou `SeDebugPrivilege`: capazes, e acima da fronteira. O mesmo principal duplica os handles do lease, injeta no processo, troca o binário do CLI e chama a AWS diretamente.

O que os controles de fato fecham, e vale manter: junction preexistente plantada por ferramenta de manutenção, redirecionamento por utilitário de mover dados de aplicativo, execução concorrente do próprio runbook e engano do operador.

O especialista retira o `BLOCKED` no que ele afirmava fechar uma ameaça. Mantém duas ressalvas de conformidade: um oráculo não discriminante e um efeito colateral fora da fronteira do experimento.

### S2. Âncora mantida ou apenas validada e liberada

A1, e o artefato em disco hoje já vai além de A2 sem comprar nada medível.

Ataque que A2 fecha e A1 deixa aberto: nenhum encontrado. O handle do arquivo, sozinho, recusa com 32 a renomeação e a exclusão de todos os ancestrais, medido até oito níveis, o que cobre a profundidade produtiva. O outro caminho de redirecionamento, `FSCTL_SET_REPARSE_POINT`, não é impedido por modo de compartilhamento em hipótese nenhuma e é impedido por não vacuidade. Todo ancestral povoado é não vazio por construção.

Risco simétrico e mais importante: `OpenDirectory` pede `FILE_LIST_DIRECTORY`, que participa da checagem de compartilhamento. Se qualquer processo alheio mantiver o perfil, `AppData` ou `Local` sem `FILE_SHARE_READ` no instante da aquisição, a nossa abertura falha, o lease converge para 252 e a matriz autorizada aborta. Quanto mais alto o lease sobe, maior a superfície de aborto por terceiro.

Emenda proposta: manter o lease protetor apenas em `<RunId>` e `aws-cli-json`, que o runbook cria e possui, e rebaixar os componentes de `C:\` até `Task6` para inspeção, preservando integralmente a validação por componente. O pino continua sendo o handle do arquivo.

### S3. Resíduos

Dentro da fronteira, com oráculo obrigatório: junction ou ponto de montagem preexistente em qualquer componente; substituição, sobrescrita, renomeação ou exclusão do arquivo entre o hash e a leitura; renomeação ou substituição de ancestral durante o lease; travessia de ponto de montagem, caminho UNC, prefixo de dispositivo e raiz não local; ACL do diretório de argumentos que não conceda controle exclusivo ao usuário.

Fora da fronteira, resíduo escrito sem oráculo:

| Id | Resíduo | Capacidade exigida | Efeito máximo | Sinal observável |
|---|---|---|---|---|
| RES-01 | Mesma identidade de usuário em integridade média | Token do usuário | Total | Nenhum |
| RES-02 | Mapa de dispositivos DOS por sessão | Token do usuário, mesma sessão | Redireciona a resolução do caminho pelo CLI | Nenhum |
| RES-03 | Redirecionamento de pasta conhecida e origem do `$StateRoot` | Token do usuário | Move a cadeia antes da validação | Divergência entre as duas fontes de âncora, se cruzadas |
| RES-04 | Ancestrais acima da âncora, sob a emenda | Token do usuário ou superior | Troca de ancestral antes da aquisição | Rejeição de nova análise; troca durante o lease recusada com 32 |
| RES-05 | SYSTEM, Administradores e privilégios de backup, restauração, propriedade e depuração | Privilégio superior | Total, incluindo duplicação dos handles do lease | Nenhum |
| RES-06 | Driver de filtro, antivírus ou agente de backup | Kernel | Aborto por 32, ou semântica diferente da medida | Convergência para 252 |
| RES-07 | Janela de diretório vazio em `aws-cli-json` antes da primeira gravação | Token do usuário | Documentos escritos através de junction, sem leitura de conteúdo alheio pelo CLI | `DirectoryChainReparsePoint` ou `DirectoryChainIdentityMismatch`, com 252 |
| RES-08 | Sistema de arquivos que não seja NTFS local | Configuração da máquina | As recusas medidas deixam de valer | Tipo do sistema de arquivos não é verificado |

Forma de registro: uma seção de resíduos aceitos no documento do experimento, com uma linha por resíduo, identificador estável, principal, capacidade, efeito máximo, sinal observável ou a palavra nenhum, e a frase de aceitação com data. Nenhum resíduo pode aparecer como pergunta aberta ou item vazio: ou é linha de resíduo aceito, ou é oráculo do ensaio.

### S4. Identidade comparada ou cadeia de handles

Nenhuma das duas isoladamente, porque provam coisas diferentes.

O par `VolumeSerialNumber` e `FileId` responde que dois nomes denotavam o mesmo objeto em um instante. É igualdade pontual. O Win32 não expõe primitiva de fixação de caminho: a abertura reexecuta a resolução nome a nome, e o AWS CLI é outro processo que abre por conta própria. Medido em P2: a comparação passou e, depois da troca do ancestral, o mesmo caminho resolveu para outro objeto.

O que o experimento precisa é que o nome resolvido depois pelo CLI denote o objeto cujo hash foi verificado. Isso decorre do conjunto de recusas que o sistema de arquivos impõe enquanto os handles existem. O handle do arquivo, com acesso de dados e sem compartilhamento de escrita nem exclusão, é o pino.

A comparação continua necessária por dois motivos que o handle não cobre: é o único detector do caso em que a troca ocorreu antes da aquisição, e é o que liga abrir alguma coisa a abrir o objeto que o caminho denota.

Detalhe que a revisão não pode deixar degradar: a identidade tem de vir da forma de 128 bits, com número de sequência do registro MFT. O índice de 64 bits não carrega sequência e é reutilizável após exclusão. O runbook já usa a forma de 128 bits.

### Oráculos exigidos antes do veredito `READY`

(a) junction preexistente no componente final e em componente ancestral; (b) substituição do arquivo durante o processo filho; (c) `FSCTL` durante o lease por `GENERIC_WRITE`, recusado com 32 na abertura; (d) novo, `FSCTL` durante o lease por `FILE_WRITE_ATTRIBUTES`, com abertura permitida e `FSCTL` recusado com 145; (e) novo, `FSCTL` em diretório mantido vazio, com sucesso registrado como `RES-07` e cobertura provada pela rejeição de nova análise e pela comparação de `FileId`; (f) novo, troca de ancestral durante o lease, recusada com 32, no nível mais profundo e no mais raso; (g) o processo filho lê os bytes verificados; (h) toda falha local converge para 252.

O oráculo atual de `FSCTL` abre com `GENERIC_WRITE` e passa hoje, mas por motivo mais estreito do que a propriedade que enuncia: mede que um acesso é recusado, não que a nova análise é impossível. Sem o oráculo (d), a fronteira registra uma proteção que não existe.

Controle negativo obrigatório: mutação em tempo de execução, não em tempo de compilação. Inverter um controle e provar que o oráculo correspondente reprova. Um oráculo que só falha porque o script deixou de ser analisado não provou nada.

### Modos de falha da própria recomendação

O argumento de redundância do lease de ancestrais é enumeração de caminhos de ataque, não prova; foram enumerados nova análise, renomeação, exclusão, substituição atômica, criação de nome concorrente, alias curto e link físico. O especialista errou uma leitura de máscara nesta sessão, e a correção veio de instrumentação, não de releitura. Todas as medições são de uma máquina, uma compilação e um conjunto de drivers. A observação de 60 segundos não sustenta nem a presença nem a ausência de colateral. O ensaio completo não foi executado, porque invoca o binário real do AWS CLI.

### Dissenso preservado

O especialista diverge do próprio parecer da 24ª revisão no ponto do fechamento de ameaça, e o mantém no ponto da assimetria entre o que o código enuncia e o que o ensaio prova. Diverge da premissa de A2 na abertura: o ganho está quantificado agora e é zero; o custo é que não estava. Permanecem sem oráculo os resíduos `RES-02`, `RES-05`, `RES-06` e `RES-08`. Confiança alta para os fatos Win32 e para o estado do artefato, média para a completude da enumeração de caminhos de ataque, e baixa para a frequência do efeito colateral.
## Síntese do mediador (`dotnet-architect`)

**Registrada em**: 2026-09-01, depois das duas posições independentes.

**Objeto da síntese**: `task-06-aws-matrix.ps1`, SHA-256 `17f3bba47be5d3da783c20e2dfb8ac622daa8b87038f904379039efb401b05d9`, 5.988 linhas, e `task-06-aws-json-transport-tests.ps1`, SHA-256 `28f498939460d9ef213a68a28736bc9c8194b920c769783651d0275e941852e2`, 821 linhas. Os dois hashes foram remedidos pelo mediador e coincidem com os publicados pelos participantes. Nenhuma chamada AWS, nenhum acesso de rede, nenhuma escrita fora deste documento.

### Procedência das posições, e uma anomalia que o dono precisa ver

As duas posições não estão anexadas a este documento. Elas estão em `C:\projects\montebravo\mb-notification-hub\task-06-round-table-json-transport.md`, na raiz do repositório, com 172 linhas, enquanto este documento, em `.araia\runs\SPEC-001\IMPLEMENT\SLICE-001\experiments\`, tinha 121 linhas e terminava na abertura do mediador. Dois arquivos de mesmo nome de base carregam metades da mesma mesa. O mediador não move nem funde nada, porque a restrição desta sessão autoriza escrita apenas neste documento. Item de dono, seção 9.

### Retificação da abertura do mediador

Verificado por leitura direta do artefato atual:

- F2 **retirado**. `FILE_SHARE_WRITE`, `FileShareWrite` e `0x00000002` não ocorrem no runbook. `OpenDirectory` (linha 447) pede `FileListDirectory | FileReadAttributes`, o que confirma a retificação do especialista e refuta a leitura de máscara inerte. A cadeia é aberta desde a raiz do volume, resolvida por `GetVolumePathName` (linha 397), com um handle por componente (linhas 753 a 775) e recusa nomeada `DirectoryChainVolumeMismatch`.
- F1 **mantido**. Linha 27: `$StateRoot = Join-Path $env:LOCALAPPDATA "Araia\Task6\$RunId"`. Não existe `GetFolderPath` no runbook.
- F3 **mantido**. O ensaio enraíza em `[System.IO.Path]::GetTempPath()` (linha 250) e não reproduz a cauda produtiva.
- Confirmado a favor do engenheiro: identidade por `GetFileIdInformationByHandle` com `FileId` de 128 bits (linhas 304 a 340); continuidade de `FileId` atravessando a janela do `icacls`, com recusa `DirectoryChainIdentityMismatch` (linhas 886 a 891); revalidação por `Assert-AwsCliArgumentLeaseCurrent` antes de cada criação de processo (linhas 955 e 1117).
- Confirmado contra o ensaio: a sonda de `FSCTL` abre apenas com `GenericWrite` (linha 102 do ensaio). Não existe variante por `FILE_WRITE_ATTRIBUTES`, nem calibração negativa em diretório não vazio, nem oráculo de renomeação de ancestral durante o lease.
- Confirmado e relevante para o escopo: `Assert-RestrictedDirectoryAcl` lê a ACL por caminho (linha 78) e só é aplicada a `<RunId>` (linha 782) e a `aws-cli-json` (linha 807). `Araia` e `Task6` são mantidos e validados, e não têm ACL verificada.

Estado das lacunas: L1 fechada por S1; L3 fechada por M3 e Y, medida de um a oito níveis; L2 fechada no mecanismo e aberta na frequência; L4 não fechada por medição e dissolvida pela fronteira da seção 3, porque quem reescreve DACL está fora dela.

### 1. Veredito

**`RECOMMEND`**, alternativa líder **A1**, sobre os dois hashes acima.

O escopo dentro de A1 fica em **`NO-CONSENSUS` declarado** e volta ao dono. A mesa não tem evidência que ordene a emenda do especialista contra o que está em disco: as duas pontas do custo estão em unidades diferentes e nenhuma das duas está quantificada em frequência.

`READY` continua bloqueado, e não pelos controles: pelos oráculos C06, C08, C09, C10 e C12 da seção 4.

### 2. Desafio delimitado

#### 2.1 As três pré-condições do engenheiro

| Pré-condição | Estado | Evidência |
|---|---|---|
| M2, calibração negativa do `FSCTL` em diretório não vazio | Satisfeita como medição, não satisfeita como oráculo | M2b: 145 pelos dois acessos; o ensaio ainda abre só com `GenericWrite` |
| M3, renomeação e exclusão de ancestral a três e a cinco níveis | Satisfeita e excedida como medição, não satisfeita como oráculo | M3 e Y: 32 de um a oito níveis, mais exclusão recursiva |
| M7, medição do colateral de renomeação | Não satisfeita | Mecanismo medido em M1c, M4 e X; frequência declarada não medida pelo próprio especialista |

Distinção que a mesa fixa: medir na sonda decide a alternativa, medir no ensaio libera o `READY`. M2 e M3 cruzaram a primeira linha e não a segunda, e por isso reaparecem como C06 e C08, não como pendência de decisão.

A pré-condição que falta ainda condiciona a decisão? Não a decisão que a mesa toma; sim a que vai ao dono. M2 e M3 já resolveram a metade de segurança da pergunta de escopo, e resolveram numa direção só: nenhum ataque foi encontrado que a cadeia larga feche e a estreita deixe aberto, e o handle do arquivo recusa com 32 em toda a profundidade produtiva. O que M7 mediria é preço, e preço só empurra para mais estreito, nunca para mais largo. Portanto M7 não pode inverter a ordem entre A1 estreita e A1 larga em segurança; ele apenas quantifica quanto custa manter o que já está em disco.

Desafio ao engenheiro: a sua lista de três não decidiria escopo nem se as três estivessem medidas. M7, como você o definiu, mede o colateral que **nós impomos a terceiros** por renomeação. Ele não mede o risco inverso, que é o argumento central do especialista: um terceiro que mantenha um ancestral sem `FILE_SHARE_READ` faz **a nossa** aquisição falhar. São dois riscos distintos, com sinais distintos, e o segundo continua sem medição nas duas listas. Ele entra como `RES-10`.

#### 2.2 A enumeração declarada como base

A mesa **aceita** a enumeração declarada, com dois limites escritos.

Primeiro limite: ela sustenta uma proposição de custo, nunca uma de fechamento de ameaça. A frase que ela autoriza é "nenhum caminho enumerado é fechado pela cadeia larga e deixado aberto pela estreita". A frase que ela não autoriza é "a cadeia estreita é segura".

Segundo limite, e a razão de aceitar: a enumeração de caminhos está subordinada a uma segunda enumeração, a de principais, em S1. A de caminhos é aberta, porque nasce da imaginação do analista. A de principais é fechada pelo modelo de controle de acesso do sistema: outra conta sem travessia no perfil, processo do mesmo usuário em integridade baixa barrado pela política de integridade obrigatória, processo do mesmo usuário em integridade média que já possui o token, e principal privilegiado que está fora da fronteira. Um caminho não enumerado só importa se existir principal capaz de exercê-lo, e a enumeração fechada diz que dentro da fronteira não existe nenhum.

Consequência registrada caso a enumeração esteja incompleta: `RES-09`, sem sinal observável. Nenhum oráculo pode fechá-lo, porque uma sonda valida o padrão que foi escrito e nunca o alvo que foi esquecido. O que substitui o oráculo é uma regra de reabertura: um caminho de ataque novo só reabre esta mesa se vier acompanhado do nome de um principal dentro da fronteira capaz de exercê-lo. Sem esse nome, é resíduo, e a 25ª revisão registra sem reprovar.

#### 2.3 `FILE_LIST_DIRECTORY` contra o custo de mexer em código sensível

O risco é real e é do tipo pior: converte um controle de segurança em falha de disponibilidade de uma execução autorizada com recursos já criados. Três observações do mediador sobre essa evidência:

- O mecanismo está medido em uma direção só. M1c e X medem o nosso handle recusando o terceiro. Ninguém mediu um terceiro recusando a nossa abertura. A inferência é sólida, porque a checagem de compartilhamento do NTFS é simétrica, mas continua sendo inferência e não observação.
- A frequência não está medida. Sessenta segundos com zero eventos não distinguem risco baixo de janela mal escolhida, e o próprio especialista recusa a inferência.
- Existe uma observação favorável ao estreitamento que nenhuma das duas posições explicitou: em B1 e P1, `GENERIC_WRITE` em `C:\` e `C:\Users` foi recusado com **5**, por ACL, e não com 32. Nesses dois componentes a ACL já nega ao usuário o que o handle negaria, o valor marginal de manter é zero e a contribuição para a superfície de aborto não é. Essa é a evidência mais forte que a emenda tem.

Do outro lado, o custo de mudar: a alteração cai em `New-AwsCliArgumentLease`, exatamente a função sob revisão, na 24ª rodada, com uma segunda sessão de agente escrevendo o mesmo arquivo. Os hashes mudaram duas vezes em quatro minutos durante esta mesa. Cada rodada custa de 20 a 40 minutos e reinicia a revisão.

Pesagem do mediador, que é recomendação e não decisão: o dano do aborto é limitado e recuperável, porque a convergência para `not-applied` com 252 é decisão aceita e a limpeza parcial já foi verificada uma vez; o dano da mudança é certo no custo e imediato no risco de corrida. Recomendo **manter nesta rodada o escopo que está em disco**, fechar os oráculos, aceitar `RES-10` e `RES-11` por escrito, e estreitar apenas se C08 reprovar ou se um aborto real ocorrer. Registro com a mesma clareza o argumento contrário: a emenda do especialista é uma **remoção**, e remoção é mais barata de revisar do que adição, o que enfraquece o meu argumento de custo. As duas pontas estão em unidades diferentes, probabilidade contra rodadas, e por isso a escolha é do dono.

#### 2.4 O que a mesa não decide

Detalhado na seção 9. Em uma linha: a mesa fixa fronteira, controles e oráculos; ela não escolhe escopo, não aceita resíduo, não autoriza gasto e não arbitra conflito entre sessões de agente.

### 3. Fronteira de ameaça, forma normativa

> **Está dentro da fronteira** todo principal que, no instante da execução, não possua o token do usuário que executa o runbook nem privilégio superior a ele, e cuja capacidade se limite a criar, renomear, excluir, substituir ou converter em ponto de nova análise qualquer componente do caminho do arquivo de argumentos, entre a verificação do hash e a leitura pelo AWS CLI; **está fora da fronteira** todo principal que possua o token desse usuário em integridade média ou superior, `SYSTEM`, membro do grupo de Administradores, portador de `SeBackupPrivilege`, `SeRestorePrivilege`, `SeTakeOwnershipPrivilege` ou `SeDebugPrivilege`, código em modo núcleo incluindo driver de filtro, antivírus e agente de backup, e toda configuração de máquina que altere a resolução do caminho antes do processo, a saber mapa de dispositivos DOS, redirecionamento de pasta conhecida e sistema de arquivos que não seja NTFS local.

Regra de uso pela 25ª revisão: a revisão julga conformidade com essa frase. Achado que dependa de capacidade listada como fora da fronteira é resíduo registrado, nunca reprovação. Ampliar a fronteira exige decisão do dono e não pode acontecer dentro de uma rodada de revisão.

### 4. Checklist fechado da 25ª revisão

Consolida R1 do engenheiro (M1 a M7) com os oráculos (a) a (h) do especialista.

| Id | Verificação | Falsificação | Origem |
|---|---|---|---|
| C01 | Os hashes SHA-256 do runbook e do ensaio, medidos no início e no fim da revisão, são iguais aos publicados nesta síntese | Hash diferente em qualquer das duas medições; nesse caso a revisão é inválida, não reprovada | Exigência de procedimento do engenheiro |
| C02 | Existe cenário que reproduz a cauda `Araia\Task6\<RunId>\aws-cli-json` sob raiz descartável, com asserção ordinal dos quatro nomes | Renomear um nome de cauda na composição do caminho sem que nenhum teste reprove | M1, F3 |
| C03 | Junction preexistente no componente final e em componente ancestral é recusada com categoria nomeada e converge para 252 | A aquisição conclui, ou conclui com categoria genérica | (a), O2, O3 |
| C04 | Substituição, sobrescrita, renomeação e exclusão do arquivo durante o processo filho são recusadas, e o processo filho lê os bytes verificados | Qualquer das quatro operações conclui, ou os bytes lidos diferem dos verificados | (b), (g), O4, O6 |
| C05 | `FSCTL_SET_REPARSE_POINT` durante o lease por `GENERIC_WRITE`: recusa na abertura com 32, sem que `DeviceIoControl` seja chamado | A abertura conclui, ou `DeviceIoControl` é chamado | (c), O5 |
| C06 | `FSCTL_SET_REPARSE_POINT` durante o lease por `FILE_WRITE_ATTRIBUTES`: abertura permitida e `DeviceIoControl` recusado com 145. **Ausente hoje** | `DeviceIoControl` devolve sucesso, ou a abertura falha, o que tornaria o oráculo não discriminante outra vez | (d), M2 |
| C07 | Calibração positiva em diretório mantido vazio: o `FSCTL` conclui, o sucesso é registrado como `RES-07` e a cobertura é provada pela rejeição de nova análise e pela comparação de `FileId` | A calibração falha, o que invalida C05 e C06 por ausência de controle positivo | (e), O1 |
| C08 | Troca de ancestral durante o lease, renomeação e exclusão, recusadas com 32 no componente mantido mais raso e no mais profundo. **Ausente hoje** | Qualquer das duas conclui em qualquer dos dois níveis; se reprovar no mais raso, a emenda de escopo deixa de ser preferência e passa a ser obrigatória | (f), M3 |
| C09 | A comparação de identidade usa `VolumeSerialNumber` mais `FileId` de 16 bytes | Substituir pelo índice de 64 bits sem que nenhum oráculo reprove | S4 |
| C10 | A aquisição recusa, com categoria nomeada e 252, cadeia em que um componente conceda escrita a principal que não seja o usuário atual. **Ausente hoje** | A aquisição conclui. Reprovar em máquina com política corporativa é falha fechada legítima, não defeito do controle | M6 |
| C11 | Cada categoria de recusa local nomeada produz `not-applied` com código 252, com pelo menos uma execução por categoria | Uma categoria alcança o AWS CLI, ou produz código diferente de 252 | (h), O8 |
| C12 | Controle negativo por mutação em tempo de execução: para cada campo verificado do lease e para a revalidação anterior à criação do processo, inverter o controle em execução e provar que o oráculo correspondente reprova. **Ausente hoje** | A mutação impede a análise do script ou a compilação do tipo inline; nesse caso quem reprovou foi o analisador e a asserção segue sem prova | M4, controle negativo do especialista |

Deixados de fora de propósito, com a razão registrada:

- **M5 e E1, cruzamento das duas fontes de âncora**: o redirecionamento de pasta conhecida e a origem de `$StateRoot` estão **fora** da fronteira da seção 3, porque exigem o token do usuário. Exigir controle contra ameaça fora da fronteira é a inflação que esta mesa foi convocada para parar. Vira item opcional do dono e, enquanto não existir, o sinal de `RES-03` fica corrigido para "nenhum".
- **M7**: é entrada para a decisão de escopo do dono, não condição de `READY`, pela razão da seção 2.1.
- **ACL por handle, E3**: simplificação de prova contra ameaça fora da fronteira, com custo de P/Invoke novo justamente na rodada cujo objetivo é parar de crescer.

Regra de fechamento: achado fora de C01 a C12 é resíduo ou gatilho de reabertura, nunca reprovação. Gatilho de reabertura exige o nome de um principal dentro da fronteira.

### 5. Resíduos aceitos

| Id | Resíduo | Capacidade exigida | Efeito máximo | Sinal observável |
|---|---|---|---|---|
| RES-01 | Mesma identidade de usuário em integridade média | Token do usuário | Total | Nenhum |
| RES-02 | Mapa de dispositivos DOS por sessão | Token do usuário, mesma sessão | Redireciona a resolução do caminho pelo CLI | Nenhum |
| RES-03 | Redirecionamento de pasta conhecida e origem do `$StateRoot` | Token do usuário | Move a cadeia antes da validação | Nenhum, enquanto a âncora derivar apenas de `$env:LOCALAPPDATA` (linha 27); sinal corrigido pelo mediador, porque o cruzamento de E1 ficou fora do checklist |
| RES-04 | Ancestrais acima da âncora, sob a emenda | Token do usuário ou superior | Troca de ancestral antes da aquisição | Rejeição de nova análise; troca durante o lease recusada com 32 |
| RES-05 | SYSTEM, Administradores e privilégios de backup, restauração, propriedade e depuração | Privilégio superior | Total, incluindo duplicação dos handles do lease | Nenhum |
| RES-06 | Driver de filtro, antivírus ou agente de backup | Kernel | Aborto por 32, ou semântica diferente da medida | Convergência para 252 |
| RES-07 | Janela de diretório vazio em `aws-cli-json` antes da primeira gravação | Token do usuário | Documentos escritos através de junction, sem leitura de conteúdo alheio pelo CLI | `DirectoryChainReparsePoint` ou `DirectoryChainIdentityMismatch`, com 252 |
| RES-08 | Sistema de arquivos que não seja NTFS local | Configuração da máquina | As recusas medidas deixam de valer | Tipo do sistema de arquivos não é verificado |
| RES-09 | Enumeração de caminhos de ataque declarada incompleta, criado pelo mediador | Token do usuário ou superior, pela enumeração de principais de S1 | Um caminho não enumerado contorna o pino do handle | Nenhum; o gatilho é a nomeação de um principal dentro da fronteira |
| RES-10 | Terceiro que mantenha um componente ancestral sem `FILE_SHARE_READ` no instante da aquisição, criado pelo mediador | Qualquer processo da máquina, sem privilégio | Aborto da matriz autorizada com recursos já criados | 252 com a categoria de falha de abertura de componente; frequência não medida |
| RES-11 | Colateral imposto a processos vizinhos pelos handles de diretório mantidos, criado pelo mediador | Nenhuma; é efeito do nosso próprio lease | Recusa de gravação atômica e de renomeação em processos alheios durante a matriz | Erro 32 no processo alheio, não observável pelo runbook |
| RES-12 | Duas sessões de agente escrevendo o mesmo runbook, criado pelo mediador | Escrita no diretório do experimento | O objeto revisado deixa de ser o objeto executado | Divergência de hash em C01 |

As linhas acima são propostas de aceitação. A frase de aceitação com data pertence ao dono da decisão, seção 9: o mediador não aceita resíduo em nome dele.

### 6. Alternativas rejeitadas

| Alternativa | Razão da rejeição |
|---|---|
| A2, âncora mantida sem compartilhamento de escrita | Ganho quantificado em zero: o handle do arquivo, sozinho, recusa renomeação e exclusão de ancestral com 32 até oito níveis, e o outro caminho de redirecionamento é barrado pela não vacuidade, não pelo modo de compartilhamento. Custo não é zero: `RES-10` e `RES-11`. Rejeitada como alvo, não como descrição, porque o que está em disco já vai além dela |
| A3, abandonar o transporte por arquivo | O CLI não aceita stdin nem handle herdado para esses parâmetros; a sintaxe abreviada não cobre documento de política IAM nem seletor do CloudTrail; JSON na linha de comando reintroduz exposição legível por qualquer processo do mesmo usuário e sem ACL, o que é retrocesso de segurança; e reabre as revisões 23 e 24 |
| A4, aceitar a versão atual sem alteração e executar | Rejeitada, e não pelo `BLOCKED`, que perdeu referente com a troca do artefato: rejeitada porque C06, C08, C10 e C12 não existem, e executar agora seria executar sem prova. A opção de manter o escopo que está em disco, oferecida ao dono na seção 9, não é A4: ela mantém os controles e continua exigindo o checklist inteiro |
| A1 com ACL por handle nesta rodada | Simplificação de prova contra ameaça fora da fronteira; custo de P/Invoke novo e uma estimativa que o próprio engenheiro já não afirma que cabe em uma rodada |
| A1 com cruzamento de âncora obrigatório | Controle contra ameaça que a seção 3 coloca fora da fronteira. Aceitá-lo como obrigatório reabriria a inflação de escopo que motivou a mesa |

### 7. Dissenso preservado

- `dotnet-engineer` contra `dotnet-specialist`, escopo: o engenheiro quer a cadeia mantida de `Araia` para baixo; o especialista quer o lease protetor apenas em `<RunId>` e `aws-cli-json`, com os ancestrais apenas inspecionados. Os dois rejeitam o que está em disco, que mantém a cadeia desde a raiz do volume. A mesa não resolve.
- `dotnet-specialist` contra o próprio parecer `BLOCKED` da 24ª revisão: retira a afirmação de fechamento de ameaça, porque S1 não encontra principal capaz sem a identidade do usuário; mantém as duas ressalvas de conformidade, o oráculo não discriminante e o colateral fora da fronteira do experimento.
- `dotnet-specialist` contra a premissa de A2 na abertura do mediador: o ganho agora está quantificado e é zero; o que não estava quantificado era o custo.
- `dotnet-architect` contra `dotnet-engineer`, em E1: o mediador recusa tornar o cruzamento de âncora obrigatório, por coerência com a fronteira. O engenheiro o propôs dentro do conjunto fechado.
- `dotnet-architect` contra a própria abertura: F2 retirado, L1 e L3 fechadas, L4 dissolvida por colocação na fronteira e não por medição. A abertura foi escrita sobre um artefato que já não existia.
- `dotnet-architect` sobre o congelamento: aceito como procedimento, com a ressalva que o próprio engenheiro registrou, de que congelar não impede outra sessão de escrever, apenas torna a escrita detectável. É por isso que o congelamento entra como C01, e não como promessa.
- Confiança do mediador: alta para o estado do artefato e para os fatos Win32 medidos pelos dois participantes; média para a completude da enumeração de caminhos; baixa para a frequência de `RES-10` e `RES-11`.

### 8. Consequências e experimento de validação exigido antes do `READY`

Experimento exigido, local, sem AWS e sem rede: implementar C02, C06, C08, C09, C10 e C12 no ensaio, publicar o par de hashes antes e depois, executar a suíte inteira e registrar o resultado por item do checklist. A mutação de C12 é em tempo de execução; mutação que quebre a análise do script não conta como prova.

Consequências por resultado:

- Se C06 reprovar, ou seja, se o `FSCTL` concluir por `FILE_WRITE_ATTRIBUTES` em diretório mantido e não vazio, a defesa contra nova análise em diretório mantido cai, o pino passa a depender só do handle do arquivo e a mesa reabre com evidência nova.
- Se C08 reprovar no componente mantido mais raso, a cadeia larga passa a ser ativamente nociva e a emenda do especialista deixa de ser preferência e vira obrigação.
- Se C10 reprovar em máquina com política corporativa, o controle está certo e a máquina está fora do perfil: converge para 252, e não se remove o controle.
- Se C12 não conseguir mutar em tempo de execução, o conjunto de oráculos permanece sem prova negativa e o `READY` não é emitido, mesmo com todos os outros itens verdes.

Estimativa de tamanho, marcada como inferência do mediador e não como medição: o engenheiro estimou cerca de 123 linhas no runbook e 290 no ensaio com M1 a M7. Retirando M5 e M7 e mantendo M6, a parte do runbook encolhe para algo próximo de 40 linhas e a do ensaio fica perto de 230. Cabe em uma rodada, na mesma condição que o engenheiro impôs: o conjunto tem de estar fechado antes de começar, e o conjunto é o da seção 4.

### 9. Entrega ao dono da decisão

O mediador não decide nada abaixo desta linha.

1. **D1, escopo dentro de A1**: escolher entre a emenda do especialista (lease protetor apenas em `<RunId>` e `aws-cli-json`, ancestrais só inspecionados), a proposta do engenheiro (cadeia mantida de `Araia` para baixo) e a manutenção do que está em disco (cadeia desde a raiz do volume). Recomendação do mediador: manter nesta rodada, pela seção 2.3. A evidência contrária está na mesma seção. A troca envolve disponibilidade contra minimalidade, e as duas pontas estão sem medição de frequência.
2. **D2, conflito de duas sessões de agente sobre o mesmo runbook**: durante esta mesa, `task-06-aws-matrix.ps1` e `task-06-aws-json-transport-tests.ps1` mudaram duas vezes em quatro minutos, por uma sessão concorrente que implementou a remediação da 24ª revisão. Não existe trava técnica. O dono decide quem é o dono do artefato até o `READY`, se a 25ª revisão fica bloqueada enquanto houver outra sessão ativa, e o que acontece se C01 acusar divergência no meio da revisão.
3. **D3, duplicidade de local da mesa**: as posições estão na raiz do repositório e o brief está em `.araia/runs/...`, em dois arquivos de mesmo nome de base. O dono decide qual é o documento canônico e quem funde os dois, já que esta sessão está proibida de escrever fora deste arquivo.
4. **D4, aceitação dos resíduos**: `RES-01` a `RES-12` precisam da frase de aceitação com data, que é ato de aceitação de risco e pertence ao dono. Sem ela, a seção 5 é proposta e não registro.
5. **D5, itens fora do checklist**: cruzamento de âncora (E1 e M5), ACL por handle (E3, com o oráculo honesto que afrouxa o DACL e registra que nada detecta) e verificação do tipo do sistema de arquivos, que hoje é `RES-08`. Todos são defesa em profundidade contra ameaça fora da fronteira, e cada um custa uma rodada.
6. **D6, medição opcional de M7 e de `RES-10`**: quantificar a frequência do colateral e a probabilidade do aborto por terceiro exige uma janela de observação bem maior do que sessenta segundos. O dono decide se paga esse tempo antes de D1 ou se aceita decidir sem ele.
7. **D7, registro da retirada parcial do `BLOCKED`**: a linha 61 de `.araia/refusal-log.jsonl` continua como está. A retirada do especialista está registrada nesta mesa. O dono decide se quer uma errata apontando para este documento, e o mediador não reescreve registro fechado.
8. **D8, autorização de execução depois do `READY`**: teto de USD 0,25, duas CMKs em `PendingDeletion` por sete dias e resíduo temporário de Compliance. A mesa não autoriza gasto.
