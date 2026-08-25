---
language: pt-BR
---

# Verificações da plataforma para entrada em produção

A equipe de Engenharia de Release é responsável por este comando. A equipe de SRE o executa durante o processo anterior à entrada em produção.

Execute o comando com `dotnet run --project tools/Platform.GoLiveChecks`. O comando retorna `0` somente quando nenhuma política publicada que deva um fallback tem plano de entrega sem passo posterior. Devem um fallback a classe `critical` e qualquer classe que hospede modelo publicado com finalidade de autenticação, porque o resto do desenho trata as duas como uma unidade: a varredura pede o próximo passo em ambas e o roteamento manda ambas para a fila de autenticação. Ler apenas o nome da classe deixava passar uma política `transactional` de um passo hospedando justamente os códigos com que as pessoas entram na conta. Violações resultam no código de saída `1`; a indisponibilidade da configuração, do banco de dados, do Graph ou do armazenamento do registro resulta no código de saída `2`.

O que o comando não mede continua sendo alcance em tempo de execução: ele lê o que está publicado, não a política sob a qual uma notificação em voo foi admitida.

As contagens de modelos operacionais publicados e de atribuições de `Notifications.Send.Operational` continuam sendo lidas e gravadas no registro, agora como evidência do que está ligado, e não decidem mais o código de saída. Elas reprovavam enquanto nenhum componente lia o instante de liberação de uma notificação adiada, o que deixaria uma notificação dessa classe parada sem envio e sem alarme; o scheduler passou a lê-lo. Uma fonte ilegível continua sendo erro em qualquer das três, porque um registro sem a evidência não prova nada.

Configure o comando somente por meio de variáveis de ambiente:

- `GO_LIVE_TEMPLATE_MANAGEMENT_CONNECTION_STRING`
- `GO_LIVE_GRAPH_ACCESS_TOKEN`
- `GO_LIVE_GRAPH_SERVICE_PRINCIPAL_ID`
- `GO_LIVE_RECEIPT_PATH`

O token e a string de conexão nunca aparecem no registro nem na saída do console. O token do Graph precisa de acesso de leitura à entidade de serviço de destino e às atribuições de funções de aplicativo dela.

O adaptador de banco de dados usa SQL parametrizado deliberadamente no esquema fixo TemplateManagement. A consulta de modelos relaciona `templatemanagement.template.key` a `templatemanagement.template_version.template_key`, filtra `template.class` e filtra `template_version.status`. A consulta de planos lê `templatemanagement.class_policy_version`, filtra `status`, mede o tamanho do plano dentro da coluna `definition` (que guarda o documento publicado como texto submetido para que o hash de conteúdo continue conferindo) e aceita a linha quando a classe é a configurada ou quando existe modelo publicado daquela classe com a finalidade de autenticação. Se esse esquema sob responsabilidade da equipe mudar, atualize o adaptador e os testes de contrato em conjunto.
