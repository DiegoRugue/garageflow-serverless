# GarageFlow Serverless

Funções de autenticação de clientes por CPF e autorização do API Gateway para o Tech Challenge Fase 3. A aplicação GarageFlow mantém as regras de negócio e a verificação de credenciais; a função de autenticação valida a entrada, consulta a API privada e emite o JWT do cliente.

Este repositório tem ciclo de build e deploy próprio. Metadados de infraestrutura são consumidos por contratos versionados; não há referência de projeto para outro checkout.

## Runtime .NET

O runtime usa .NET 10 (`net10.0`) no modelo de deployment por arquivo ZIP da runtime gerenciada `dotnet10`. O projeto `Host` é uma class library e contém somente tradução dos eventos AWS e composição das dependências. As regras e portas seguem para dentro pelas camadas `Domain`, `Application` e `Adapters.Infrastructure`; não há persistência nem referência à solução principal GarageFlow.

Os handlers publicados são:

- autenticação: `GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.CustomerAuthenticationFunction::FunctionHandler`
- authorizer HTTP API REQUEST 2.0: `GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.RequestAuthorizerFunction::FunctionHandler`

A função de autenticação atende `POST /auth/customers/token` no formato proxy HTTP API 2.0. O corpo JSON contém `cpf` e `password`; o CPF é validado e normalizado para 11 dígitos antes da chamada privada. A resposta de sucesso contém `token`, `mustChangePassword`, `tokenType` e `expiresIn`, com `Cache-Control: no-store`. O authorizer recebe um evento REQUEST 2.0 e devolve a resposta simples `isAuthorized`.

## Configuração do runtime

Somente ARNs dos segredos entram nas variáveis da Lambda. Os valores são buscados no AWS Secrets Manager pela cadeia normal de credenciais do runtime e permanecem em cache por no máximo 60 segundos.

| Variável | Função | Uso |
| --- | --- | --- |
| `JWT_SECRET_ARN` | ambas | ARN da chave HS256 dos JWTs de usuário |
| `INTERNAL_AUTH_SECRET_ARN` | autenticação | ARN da chave HS256 do token de serviço interno |
| `INTERNAL_API_BASE_URL` | autenticação | URL base fixa da API privada |
| `INTERNAL_API_TRANSPORT` | autenticação | `http` ou `https`, igual ao esquema da URL |
| `JWT_ISSUER` | ambas | emissor do JWT; padrão `GarageFlow` |
| `JWT_AUDIENCE` | ambas | audiência do JWT; padrão `GarageFlow.Adapters.Api` |

As chaves devem ter pelo menos 32 bytes UTF-8, não podem ser placeholders e devem ser diferentes. A operação HTTP tem limite de 5 segundos incluindo a leitura do corpo, não segue redirects e não repete solicitações. As duas funções propagam um prazo baseado no tempo restante da invocação, reservando 1 segundo para responder; esse prazo também abrange as consultas ao Secrets Manager. Ao esgotar o prazo, a autenticação retorna `503 authentication_unavailable` e o authorizer nega acesso. Erros de configuração, Secrets Manager, transporte ou identidade inválida não incluem credenciais, tokens ou respostas privadas na resposta HTTP.

## Build, testes e pacote

```powershell
dotnet build GarageFlow.Serverless.slnx --configuration Release
dotnet test Tests/Unit/GarageFlow.Serverless.Tests.Unit.csproj --configuration Release
dotnet test Tests/Integration/GarageFlow.Serverless.Tests.Integration.csproj --configuration Release
dotnet test GarageFlow.Serverless.slnx --configuration Release --settings coverlet.runsettings --collect:"XPlat Code Coverage"
dotnet publish Host/GarageFlow.Serverless.Host.csproj --configuration Release --runtime linux-x64 --self-contained false --output <diretorio-externo>
```

Testes de integração usam handlers HTTP controlados, tokens JWT reais e mocks do SDK do Secrets Manager. Eles não exigem credenciais AWS nem acesso à rede.

## Infraestrutura serverless

O módulo em `infra/serverless` usa Terraform `1.15.7` e AWS provider `6.49.0`. Ele cria duas funções Lambda `dotnet10` na arquitetura `x86_64`, publica uma versão imutável de cada função e mantém o alias `live` como destino estável. A função de autenticação usa 512 MB, timeout de 10 segundos e as sub-redes privadas de aplicação com o security group fornecido pelo contrato de ingress. O authorizer usa 256 MB, timeout de 5 segundos e permanece fora da VPC. Ambos os log groups retêm dados por sete dias.

O módulo não cria IAM. `authentication_role_arn` e `authorizer_role_arn` recebem roles preexistentes e podem apontar para o mesmo `LabRole`. Antes do plano, o deploy confirma a conta do caller e de todos os ARNs, a existência e identidade das roles, a associação das sub-redes e security groups à VPC, e que o listener pertence a um ALB interno. Em HTTP, o hostname da URL deve ser o DNS do ALB; em HTTPS, deve coincidir com `tlsServerName`. Somente ARNs de segredos entram nas variáveis das funções; valores de segredos não são lidos pelo deploy.

As entradas Terraform são `environment`, `owner`, `expires_on`, `platform_contract`, `ingress_contract`, `authentication_role_arn`, `authorizer_role_arn` e `package_path`. O contrato de plataforma v1 é consumido por completo. O contrato de ingress v2 inclui `listenerArn`, `internalApiBaseUrl`, `transport`, `authenticationSecurityGroupId`, `vpcLinkSecurityGroupId` e, somente para HTTPS, `tlsServerName`. As únicas saídas públicas são:

- `customerAuthenticationAliasArn`
- `requestAuthorizerAliasArn`

Testes locais do módulo usam exclusivamente o mock provider:

```powershell
terraform -chdir=infra/serverless fmt -check -recursive
terraform -chdir=infra/serverless init -backend=false -input=false
terraform -chdir=infra/serverless validate
terraform -chdir=infra/serverless test -no-color
```

## Deploy protegido

O workflow de deploy aceita somente `develop` para `homologation` e `main` para `production`, sempre em `us-east-1`. Pushes nessas branches passam primeiro pelo quality gate. A execução manual exige o ambiente correspondente à branch selecionada e a confirmação do SHA completo. Os dois GitHub Environments protegidos deste repositório, `homologation` e `production`, devem fornecer:

| Tipo | Nome |
| --- | --- |
| Secret | `AWS_ACCESS_KEY_ID` |
| Secret | `AWS_SECRET_ACCESS_KEY` |
| Secret | `AWS_SESSION_TOKEN` |
| Secret | `TF_STATE_BUCKET` |
| Secret | `AUTHENTICATION_ROLE_ARN` |
| Secret | `AUTHORIZER_ROLE_ARN` |
| Variable | `AWS_ACCOUNT_ID` |
| Variable | `TF_OWNER` |
| Variable | `TF_EXPIRES_ON` |

Antes de habilitar este workflow, o reusable workflow centralizado `deploy-edge.yml` da plataforma deve estar mergeado na branch `main` de `DiegoRugue/garageflow-infra-kubernetes`. Os dois callers usam essa fonte `@main`; nas chamadas entre repositórios, a plataforma valida seu código centralizado em `main` e implanta exatamente o SHA aprovado pelo quality gate. O ref protegido do caller seleciona o Environment e o state. Neste repositório serverless, `main` e `develop` devem existir e estar protegidas, e `develop` precisa ser criada durante o setup caso ainda não exista. Os Environments reais correspondentes são `production` e `homologation`.

O script baixa `contracts/v1/{environment}/platform.json` e `contracts/v2/{environment}/ingress.json` do bucket de estado para `RUNNER_TEMP`, valida os dois contratos e gera os tfvars apenas nesse diretório temporário. O pacote é criado fora do checkout com o SDK `.NET 10.0.301`:

```bash
dotnet publish Host/GarageFlow.Serverless.Host.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false \
  --output "$RUNNER_TEMP/publish"
```

O estado usa a chave `phase3/{environment}/serverless.tfstate`, criptografia e lock nativo do S3. O apply consome exatamente o plano salvo. Após as duas funções ficarem ativas, o script publica primeiro a revisão imutável `contracts/v1/{environment}/serverless/revisions/{sha}/{run-id}-{run-attempt}.json` com `If-None-Match: *` e depois atualiza `contracts/v1/{environment}/serverless.json`. Assim, uma recuperação pode repetir o mesmo commit em outra execução sem colidir com a revisão anterior. Só então os dois callers protegidos chamam o deploy reutilizável do edge da plataforma confiável, sob o mesmo proprietário, com `secrets: inherit`. Essa herança explícita permite resolver os secrets de deployment na chamada entre repositórios. O workflow centralizado usa a fonte `@main`, declara o mesmo Environment protegido selecionado pelo ref do caller e lê as credenciais e variáveis configuradas neste repositório, garantindo que a borda consuma aliases já publicados.
