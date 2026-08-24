# Verificações da plataforma para entrada em produção

A equipe de Engenharia de Release é responsável por este comando. A equipe de SRE o executa durante o processo anterior à entrada em produção.

Execute o comando com `dotnet run --project tools/Platform.GoLiveChecks`. O comando retorna `0` somente quando não há nenhum modelo operacional publicado nem atribuição de `Notifications.Send.Operational`. Violações resultam no código de saída `1`; a indisponibilidade da configuração, do banco de dados, do Graph ou do armazenamento do registro resulta no código de saída `2`.

Configure o comando somente por meio de variáveis de ambiente:

- `GO_LIVE_TEMPLATE_MANAGEMENT_CONNECTION_STRING`
- `GO_LIVE_GRAPH_ACCESS_TOKEN`
- `GO_LIVE_GRAPH_SERVICE_PRINCIPAL_ID`
- `GO_LIVE_RECEIPT_PATH`

O token e a string de conexão nunca aparecem no registro nem na saída do console. O token do Graph precisa de acesso de leitura à entidade de serviço de destino e às atribuições de funções de aplicativo dela.

O adaptador de banco de dados usa SQL parametrizado deliberadamente no esquema fixo TemplateManagement. Ele relaciona `templatemanagement.template.key` a `templatemanagement.template_version.template_key`, filtra `template.class` e filtra `template_version.status`. Se esse esquema sob responsabilidade da equipe mudar, atualize o adaptador e os testes de contrato em conjunto.
